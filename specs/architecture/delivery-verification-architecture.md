# Delivery Verification System Architecture

## Version
1.0.0 | 2026-02-21

## Status
Implemented (R4.2 — 95%, photo upload endpoint pending)

---

## 1. Component Overview

```
┌──────────────────────────────────────────────────────────────┐
│                       MOBILE (Flutter)                        │
│                                                               │
│  ┌──────────────────┐  ┌────────────────┐  ┌──────────────┐ │
│  │ DeliveryBloc      │  │ Delivery       │  │   Isar DB    │ │
│  │                   │──│ Repository     │──│  (Delivery   │ │
│  │ Events/States     │  │ (offline-first)│  │   schema)    │ │
│  └──────────────────┘  └───────┬────────┘  └──────────────┘ │
│                                │                              │
│  ┌──────────────────┐  ┌──────┴────────┐  ┌──────────────┐  │
│  │ QR Verification   │  │  SyncQueue    │  │ Photo Manager│  │
│  │ Service           │  │  (delivery    │  │ (WebP, local │  │
│  │ (HMAC-SHA256,     │  │   ops)        │  │  store)      │  │
│  │  offline)         │  └──────┬────────┘  └──────────────┘  │
│  └──────────────────┘         │                               │
│  ┌──────────────────┐         │           ┌──────────────┐   │
│  │ GPS Capture       │         │           │ Device Signing│   │
│  │ Service           │         │           │ Key Service   │   │
│  │ (Geolocator)      │         │           │               │   │
│  └──────────────────┘         │           └──────────────┘   │
└───────────────────────────────┼──────────────────────────────┘
                                │
               POST /sync (delivery ops)
               GET /deliveries
               POST /deliveries/{id}/photos
                                ▼
┌──────────────────────────────────────────────────────────────┐
│                   SERVER (.NET 10 AOT)                         │
│  SyncFunction: ProcessDeliveryAsync                           │
│  DeliveryFunction: GET + photo upload                         │
│  GpsValidator: Haversine distance + Ethiopian bounds          │
│  StatusTransitionValidator                                    │
└──────────────────────────────┬───────────────────────────────┘
                               ▼
┌──────────────────────────────────────────────────────────────┐
│  DynamoDB: DeliveriesTable (PK=deliveryId, SK=tripId)        │
│  GSI: StatusCreatedAtIndex, TripIdIndex, ListingIdIndex      │
│  S3: uploads/photos/{deliveryId}/{type}-{ts}.webp            │
└──────────────────────────────────────────────────────────────┘
```

---

## 2. QR Code Verification (Offline)

### Architecture Decision: HMAC-SHA256 over JWT

| Factor | HMAC-SHA256 (Chosen) | JWT |
|--------|---------------------|-----|
| Payload size | ~200 bytes | ~500+ bytes |
| QR readability | Better (smaller data) | Worse on cheap cameras |
| Offline verify | Yes | Yes |
| Complexity | Simple | Overkill |
| Dependencies | dart:crypto (built-in) | External package |

### QR Token Flow

```
FARMER'S DEVICE                          TRUCKER'S DEVICE
───────────────                          ─────────────────

1. Delivery created (offline)
   │
   ▼
[QR Generator]
  ├── secret = SHA256(deliveryId:tripId:listingId:creationTs)
  ├── message = did|tid|lid|fid|ts
  ├── hmac = HMAC-SHA256(secret, message)
  ├── payload = {did, tid, lid, fid, ts, hmac}
  └── qrToken = base64url(JSON(payload))
   │
   ▼
[QR Code displayed]  ──── Trucker scans ────►  [QR Scanner]
                                                  ├── Decode base64url
                                                  ├── Parse JSON
                                                  ├── Look up delivery in Isar
                                                  ├── Recompute HMAC
                                                  ├── Compare → valid/invalid
                                                  └── If valid: update status → picked_up
                                                     Enqueue sync
```

### Security Properties
- HMAC secret is delivery-specific (derived from metadata)
- Replay attacks prevented (deliveryId is in HMAC payload)
- No network required for generation or verification

---

## 3. GPS Validation

### Haversine Distance

Implemented both client-side (immediate feedback) and server-side (authoritative).

### Thresholds

| Check | Threshold | Rationale |
|-------|-----------|-----------|
| Pickup GPS vs crop location | 5 km | GPS inaccuracy + farm-to-road distance |
| Dropoff GPS vs destination woreda | 5 km | Market area size |
| Ethiopian bounds sanity | 3-15°N, 33-48°E | Reject wrong coordinates |

### Graceful Degradation

```
GPS Available     → Capture + validate + store
GPS Unavailable   → Allow operation, flag requires_review on server
GPS Outside Range → Reject, allow retry with explanation
```

---

## 4. Photo Evidence

### Pipeline

```
Camera (JPEG) → Resize (800px max) → WebP (75% quality)
  → Store local: {appDir}/photos/{deliveryId}/{type}-{ts}.webp
  → Isar: update delivery with local path
  → Upload queue (separate from sync, lower priority)
  → POST /deliveries/{id}/photos (multipart)
  → S3: uploads/photos/{deliveryId}/{type}-{ts}.webp
  → Delete local file after upload
```

### Constraints

| Parameter | Value |
|-----------|-------|
| Format | WebP only |
| Max width | 800px |
| Max file size | 200KB |
| Max per type | 3 (pickup: 3, dropoff: 3) |
| Compression | 75% quality |

---

## 5. Delivery Status Transitions

```
pending → picked_up     (trucker scans farmer's QR)
pending → cancelled     (trucker/farmer cancels)
picked_up → in_transit  (trucker starts journey)
picked_up → cancelled   (trucker cancels after pickup)
in_transit → delivered   (buyer confirms receipt)
delivered → disputed     (within 48h dispute window)

Terminal states: delivered (after dispute window), cancelled
```

Server-side enforcement via `ValidTransitions` dictionary prevents invalid state changes.

---

## 6. Screens

| Screen | Purpose | User |
|--------|---------|------|
| DeliveryListScreen | "My Deliveries" list | All roles |
| DeliveryDetailScreen | Status, GPS, photos, actions | All roles |
| QrGenerateScreen | Display QR code for handoff | Farmer |
| QrScanScreen | Camera scan for verification | Trucker/Buyer |
| DisputeScreen | Raise dispute with reason | Farmer/Buyer |

### Status Indicator (Low-Literacy)
- Color-coded icons for each status
- Large status chip with icon + localized text
- Progress timeline visualization

---

## 7. Server Implementation

### SyncFunction — Delivery Handler
```
ProcessDeliveryAsync():
  1. Validate participant authorization
  2. Field whitelist check
  3. Status transition validation
  4. GPS proximity check (Haversine)
  5. DynamoDB UpdateItem with version check
```

### DeliveryFunction
- GET /deliveries — browse with filters
- GET /deliveries/{id} — single delivery detail
- POST /deliveries/{id}/photos — photo upload (multipart)

### AOT Compliance
All DTOs registered in AppJsonContext. Manual DynamoDB marshalling. No reflection.

---

## 8. Offline-First Operations

| Operation | Offline | Sync Method |
|-----------|---------|-------------|
| Create delivery | Full (Isar + QR) | POST /sync |
| Generate QR | Full (HMAC local) | None needed |
| Scan QR | Full (HMAC verify local) | None needed |
| Capture GPS | Full (stored in Isar) | POST /sync |
| Capture photo | Full (WebP local) | POST /deliveries/{id}/photos |
| Update status | Full (Isar update) | POST /sync |
| Browse deliveries | Full (Isar cache) | Refresh on sync |

---

## 9. Security

| Concern | Mitigation |
|---------|-----------|
| QR token forgery | HMAC-SHA256 with delivery-specific secret |
| Replay attacks | deliveryId embedded in HMAC payload |
| GPS spoofing | GPS is evidence, not sole verification (QR is primary) |
| Photo tampering | Uploaded with delivery context + audit trail |
| Unauthorized status changes | JWT sub checked against participant role |
| Phone privacy | Only visible to delivery participants |

---

## 10. File Structure

```
mobile/lib/features/delivery_verification/
├── data/
│   ├── repositories/
│   │   └── delivery_repository.dart
│   └── services/
│       ├── qr_verification_service.dart
│       ├── gps_capture_service.dart
│       ├── photo_manager.dart
│       └── device_signing_key_service.dart
└── presentation/
    ├── bloc/
    │   ├── delivery_bloc.dart
    │   ├── delivery_event.dart
    │   └── delivery_state.dart
    ├── screens/
    │   ├── delivery_list_screen.dart
    │   ├── delivery_detail_screen.dart
    │   ├── qr_generate_screen.dart
    │   ├── qr_scan_screen.dart
    │   └── dispute_screen.dart
    └── widgets/
        └── delivery_status_indicator.dart
```

---

**Cross-references**:
- Detailed design: `specs/decisions/r4-2-delivery-verification-architecture.md`
- Implementation plan: `specs/decisions/f12-implementation-plan.md`
- API contract: `specs/contracts/delivery-verification-api.md`

**Last Updated**: 2026-02-21
