# Trucker View — WeredaConnect Delivery Verification

## Overview

When a user logs into WeredaConnect as a **Trucker**, they see the **Deliveries** tab — a real-time dashboard of every shipment assigned to them. The trucker is the central logistics link between farmers and buyers, responsible for picking up produce, transporting it, and handing it off at the destination.

The trucker's workflow is built around two core actions: **Scan QR to confirm pickup** and **Mark In Transit**. Both work fully offline using the device's local Isar database, with changes synced to the server when connectivity is available.

---

## Deliveries Tab — List Screen

The trucker's main screen is a scrollable list of delivery cards, with two rows of filter chips at the top.

```
+--------------------------------------------------+
|  My Deliveries                                   |
+--------------------------------------------------+
| Role:  [All] [Farmer] [*Trucker*] [Buyer]        |
| Status:[All] [Pending] [Picked Up] [In Transit]  |
|        [Delivered] [Disputed]                     |
+--------------------------------------------------+
|                                                   |
|  +----------------------------------------------+|
|  | [box]  Delivery #a1b2c3d4                     ||
|  |        Abebe Bekele -> Addis Wholesale        ||
|  |        You (Trucker) . Feb 21, 2026  [Pending]||
|  +----------------------------------------------+|
|                                                   |
|  +----------------------------------------------+|
|  | [truck] Delivery #e5f6g7h8                    ||
|  |         Mulugeta Tadesse -> Merkato Traders   ||
|  |         You (Trucker) . Feb 21, 2026 [PickdUp]||
|  +----------------------------------------------+|
|                                                   |
|  +----------------------------------------------+|
|  | [route] Delivery #i9j0k1l2                    ||
|  |         Alemayehu Worku -> Bahir Dar Produce  ||
|  |         You (Trucker) . Feb 21, 2026[InTrans] ||
|  +----------------------------------------------+|
|                                                   |
|  +----------------------------------------------+|
|  | [check] Delivery #m3n4o5p6                    ||
|  |         Girma Desalegn -> Hawassa Market      ||
|  |         You (Trucker) . Feb 21, 2026[Deliver] ||
|  +----------------------------------------------+|
|                                                   |
+--------------------------------------------------+
```

### Filter Chips

**Role filters** (top row): `All | Farmer | Trucker | Buyer`
- Auto-selects **Trucker** on login based on auth role
- Filters deliveries where the current user is the trucker

**Status filters** (second row): `All | Pending | Picked Up | In Transit | Delivered | Disputed`
- Narrows the list to a specific delivery stage
- Useful when a trucker has many active deliveries

### Delivery Card Anatomy

Each card displays:
- **Status icon** — color-coded 48x48 container (large for low-literacy users on small screens)
  - Pending: orange box icon
  - Picked Up: blue truck icon
  - In Transit: indigo route icon
  - Delivered: green checkmark icon
  - Disputed: red warning icon
  - Cancelled: grey cancel icon
- **Delivery ID** — first 8 characters of the UUID
- **Route** — `{farmerName} -> {buyerName}`
- **Trucker & date** — `You (Trucker) . {date}`
- **Status chip** — colored badge on the right

Tapping a card opens the **Delivery Detail Screen**.

---

## Delivery Detail Screen

The detail screen shows the full delivery lifecycle with a **status timeline**, participant details, evidence sections, and context-sensitive action buttons.

### Status Timeline

A horizontal progress indicator showing 4 stages:

```
  (Pending)-----(Picked Up)-----(In Transit)-----(Delivered)
     [*]------------[*]--------------[ ]---------------[ ]
```

Completed stages are filled with the primary color. The current stage has a bold border. Future stages are dimmed.

### Participants Section

```
+--------------------------------------------------+
|  Details                                         |
|                                                   |
|  [agriculture]  Farmer                            |
|                 Abebe Bekele                      |
|                                                   |
|  [truck]        Trucker                           |
|                 You (Trucker)                     |
|                                                   |
|  [store]        Buyer                             |
|                 Addis Wholesale                   |
+--------------------------------------------------+
```

### Pickup Evidence Section

Shows pickup timestamp, GPS coordinates (if captured), and photo evidence. Before pickup, displays "Not yet picked up" in muted text.

```
+--------------------------------------------------+
|  Pickup                                          |
|                                                   |
|  [clock]  Time                                    |
|           Feb 21, 2026 2:30 PM                   |
|                                                   |
|  [pin]    GPS                                     |
|           9.0192, 38.7525                        |
|                                                   |
|  +--------+                                       |
|  | [photo]|  Pickup photo                        |
|  +--------+                                       |
+--------------------------------------------------+
```

### Delivery Evidence Section

Same layout as pickup — shows delivery confirmation timestamp, GPS, and dropoff photo. Before delivery, displays "Not yet delivered".

---

## Trucker Actions by Delivery Status

The trucker sees different action buttons depending on the delivery's current status:

### 1. Pending — "Scan QR (Confirm Pickup)"

When a delivery is **Pending** and the user is the trucker, the detail screen shows:

```
+--------------------------------------------------+
|  [qr_scanner]  Confirm Pickup                    |
|  ================================================|
|                                                   |
|  [cancel]  Cancel Delivery                       |
+--------------------------------------------------+
```

**Scan QR (Confirm Pickup)** opens the QR Scan Screen for pickup verification.

#### QR Scan Screen — Pickup Flow

**Phase 1: Scanning**

The trucker points the camera at the farmer's QR code. The screen shows a live camera preview with a scan frame overlay and instruction text.

```
+--------------------------------------------------+
|  Scan QR (Confirm Pickup)            [flash]     |
+--------------------------------------------------+
|                                                   |
|           +------------------+                    |
|           |                  |                    |
|           |   [camera view]  |                    |
|           |                  |                    |
|           +------------------+                    |
|                                                   |
|  +----------------------------------------------+|
|  |  Point camera at the farmer's QR code        ||
|  +----------------------------------------------+|
|                                                   |
+--------------------------------------------------+
```

- Flash toggle button in the app bar (essential for early-morning/late-evening pickups in rural areas)
- Uses offline HMAC-SHA256 verification — no internet needed
- Verification happens locally against the delivery's QR token

**Phase 2: Verified**

After successful scan, the screen shows a green checkmark, GPS capture status, photo capture option, and confirm button.

```
+--------------------------------------------------+
|  Scan QR (Confirm Pickup)                        |
+--------------------------------------------------+
|                                                   |
|              [green checkmark]                    |
|              QR Verified!                         |
|              Delivery #a1b2c3d4                   |
|                                                   |
|  +----------------------------------------------+|
|  | [pin] GPS Location                            ||
|  |       9.0192, 38.7525                         ||
|  |       Accuracy: 12m                           ||
|  +----------------------------------------------+|
|                                                   |
|  +--------+                                       |
|  | [cam]  |  Add Photo                           |
|  | photo  |  Pickup photo                        |
|  +--------+                                       |
|                                                   |
|  [========= Confirm Pickup ==================]   |
|                                                   |
+--------------------------------------------------+
```

- **GPS capture** starts automatically after QR verification
  - Shows coordinates + accuracy when available
  - Shows orange warning if GPS unavailable (still allows confirmation — offline-first)
- **Photo evidence** — optional but recommended
  - Tap the placeholder to open camera
  - Photo is compressed and stored locally
  - 120x120 thumbnail preview after capture
- **Confirm Pickup** button dispatches `ConfirmPickup` event with GPS, photo, and scanned QR nonce

**Phase 3: Invalid QR**

If the QR code doesn't match the delivery, shows a red cancel icon with error message and "Try Again" button to reset the scanner.

### 2. Picked Up — "Mark In Transit"

When a delivery is **Picked Up** and the user is the trucker:

```
+--------------------------------------------------+
|  [route]  Mark In Transit                        |
|  ================================================|
|                                                   |
|  [cancel]  Cancel Delivery                       |
+--------------------------------------------------+
```

Tapping **Mark In Transit** shows a confirmation dialog:

```
+------------------------------------------+
|  Mark In Transit                          |
|                                           |
|  Confirm that this delivery is now        |
|  in transit to the buyer?                 |
|                                           |
|           [Cancel]    [Mark In Transit]   |
+------------------------------------------+
```

This is a one-tap action (no QR scan needed) because the trucker has already verified possession at pickup. The button shows a loading spinner while processing.

### 3. In Transit — No trucker action

When a delivery is **In Transit**, the trucker sees the detail screen in read-only mode. The next action belongs to the **buyer**, who must scan a QR code to confirm delivery.

### 4. Delivered — Read-only

The delivery is complete. The trucker can view all evidence (pickup GPS, pickup photo, delivery GPS, delivery photo, timestamps) but has no action buttons.

---

## Cancel Delivery

Available when the delivery is **Pending** or **Picked Up**. Any participant (farmer, trucker, or buyer) can cancel.

```
+------------------------------------------+
|  Cancel Delivery                          |
|                                           |
|  Are you sure you want to cancel this     |
|  delivery? This action cannot be undone.  |
|                                           |
|           [Cancel]    [Cancel Delivery]   |
+------------------------------------------+
```

The cancel button is styled in red (outlined) to indicate a destructive action.

---

## Offline Behavior

The **Offline Banner** appears at the top of both the list and detail screens when the device has no connectivity:

```
+--------------------------------------------------+
|  [!] You are offline. Changes will sync later.   |
+--------------------------------------------------+
```

All trucker actions work fully offline:
- **QR scanning** uses offline HMAC-SHA256 — no server roundtrip
- **GPS capture** uses device sensors only
- **Photo capture** stores locally on the device
- **State transitions** write to Isar immediately and enqueue a sync operation
- When connectivity returns, the sync queue replays all pending operations to the server

---

## Evidence Chain for Dispute Resolution

The trucker's actions create a verifiable evidence chain:

| Stage | Evidence Captured | Captured By |
|-------|------------------|-------------|
| Pickup | QR nonce (HMAC-verified), GPS coordinates, timestamp, optional photo | Trucker |
| In Transit | Timestamp | Trucker |
| Delivery | QR nonce (HMAC-verified), GPS coordinates, timestamp, optional photo | Buyer |

This evidence chain supports the 48-hour dispute window after delivery confirmation, where farmers or buyers can raise disputes if goods are damaged or terms weren't met.

---

## Mock Data for Demo

In debug mode, the app seeds 4 trucker-perspective deliveries via `seedTruckerMockDeliveries()`:

| # | Farmer | Buyer | Status | QR Token |
|---|--------|-------|--------|----------|
| 1 | Abebe Bekele | Addis Wholesale | Pending | debug-seed-trucker-1 |
| 2 | Mulugeta Tadesse | Merkato Traders | Picked Up | debug-seed-trucker-2 |
| 3 | Alemayehu Worku | Bahir Dar Produce | In Transit | debug-seed-trucker-3 |
| 4 | Girma Desalegn | Hawassa Market | Delivered | debug-seed-trucker-4 |

This gives a trucker demo user one delivery at each lifecycle stage, showcasing every action button and evidence state.

---

## Key Technical Details

- **State machine**: `pending -> pickedUp -> inTransit -> delivered` (with `disputed` and `cancelled` branches)
- **Role enforcement**: `confirmPickup` and `markInTransit` validate that the caller is the assigned trucker; `confirmDelivery` validates the caller is the buyer
- **QR verification**: Offline HMAC-SHA256 via `QrVerificationService` + device signing keys
- **Storage**: Isar local database with `SyncQueue` for eventual consistency
- **Target devices**: Android 8+ (API 26+), 2GB RAM, low-end $50 phones
- **Localization**: All UI strings use `AppLocalizations` (Amharic, Afan Oromo, English)
