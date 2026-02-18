# TASK.md - Current Mission

## Current Status

**Phase R4: IN PROGRESS** (started Feb 17, 2026)
**Branch:** `main`
**Current:** F-12 Delivery Verification — code complete, awaiting commit + PR
**Blocking:** F-13 Telebirr Escrow (unblocked — F-12 delivery confirmation works)

---

## Phase Summary

| Phase | Status | Completion |
|-------|--------|------------|
| R1: Security Foundation | ✅ 100% | Feb 2026 |
| R2: Auth + Data Models | ✅ 100% | Feb 14, 2026 |
| R3: Sync + First Feature | ✅ 100% (20/20 tasks) | Feb 15, 2026 |
| R4: Trips + Delivery + Escrow | 🔧 ~55% (F-11 ~70%, F-12 ~95%) | Target: Apr 30, 2026 |

**R3 Achievements:** Offline-first sync engine, crop listing (create + browse), 228 server tests + 12 integration tests, all deployed to AWS (me-south-1), zero data loss validated.

**Deferred items:** F-01 (git history purge — runbook ready, awaiting execution), F-05 (cert pinning — real hashes extracted, activation planned).

Completed task details: `docs/archive/completed-tasks.md`

---

## Phase R4: Trip Listings + Delivery Verification + Telebirr Escrow

**Target:** Apr 1 - Apr 30, 2026 (4 weeks)
**Prerequisites:** R3 merged to main ✅

### R4 Feature Progress

| Feature | Server | Mobile Data | Mobile UI | Infra | Overall |
|---------|--------|-------------|-----------|-------|---------|
| F-11 Trip Listings | ✅ Read API + Sync | ✅ Schema + BLoC + Repo | ✅ 3 screens | ✅ Table + Lambda + Routes | ~70% |
| F-12 Delivery Verification | ✅ Read API + Sync + 4 sec fixes | ✅ Schema + BLoC + Repo + 3 services | ✅ 5 screens + 3 widgets + l10n | ✅ Table + Lambda + Routes | ~95% |
| F-13 Telebirr Escrow | ❌ | ❌ | ❌ | ❌ | 0% |
| F-14 Fraud Detection | ❌ | ❌ | ❌ | ❌ | 0% |
| F-15 Conflict Resolution UI | ❌ | ❌ | ❌ | — | 0% |

### R4 Exit Criteria

- [x] Transport provider can create trip listing (route, capacity, departure date)
- [x] Farmer can browse trips
- [x] Driver can verify delivery with QR + GPS + photo
- [ ] Farmer can browse trips and request delivery (needs trip matching)
- [ ] Telebirr escrow holds funds until delivery confirmed by farmer
- [ ] All entities sync offline-first (trips ✅, deliveries ✅, payments ❌)
- [ ] Integration tests validate escrow flow

---

## ACTIVE TASK: Complete F-12 Delivery Verification

**Priority:** CRITICAL — blocks F-13 escrow (highest-scored feature, score 19)
**Architecture:** `specs/decisions/r4-2-delivery-verification-architecture.md`
**Contract:** `specs/contracts/delivery-verification-api.md`

### What Exists (Done)

- Server: `DeliveryFunction.cs` — GET /deliveries, GET /deliveries/{id} with participant auth
- Server: `SyncFunction.cs` — delivery create/update handler with GPS, QR, status validation
- Server: `DeliveryRepository.cs` + `DeliveryDtos.cs` — DynamoDB data access
- Mobile: `delivery.dart` — Isar schema with full fields
- Mobile: `delivery_bloc.dart` — events/states for all delivery actions
- Mobile: `delivery_repository.dart` — offline-first CRUD with state machine
- Infra: Deliveries DynamoDB table with StatusCreatedAtIndex, TripIdIndex, ListingIndex, StatusIndex GSIs
- Security review completed for F-12

### What Must Be Built (Tasks)

#### Phase 1: Core Services (no UI dependencies) ✅
- [x] **T1**: `QrVerificationService` — HMAC-SHA256 token generation + offline verification (nonce-based F12-SEC-01)
- [x] **T2**: `GpsCaptureService` — GPS coordinates with graceful degradation (Ethiopian bounds, Haversine)
- [x] **T3**: `PhotoManager` — Capture, compress (WebP <200KB), local storage
- [x] **Server security**: 4 fixes (F12-SEC-02 dispute window, F12-SEC-04 phone regex, F12-SEC-05 GPS proximity, F12-SEC-06 dispute length)
- [x] **Packages**: 6 Flutter packages + 4 Android permissions added

#### Phase 2: Presentation Layer ✅
- [x] **T4**: `DeliveryListScreen` — role chips + status filter + RefreshIndicator
- [x] **T5**: `DeliveryDetailScreen` — status timeline, participants, action buttons
- [x] **T6**: `QrGenerateScreen` — QrImageView with HMAC token, regenerate, offline info
- [x] **T7**: `QrScanScreen` — MobileScanner, GPS auto-capture, photo capture, confirm
- [x] **T8**: `DisputeScreen` — 48h countdown, reason field, confirmation dialog
- [x] **Widgets**: DeliveryCard, DeliveryStatusIndicator, PhotoEvidenceWidget

#### Phase 3: Integration & Polish (partial)
- [x] **T9**: Localization — 59 strings × 3 languages (en/am/om)
- [x] **T10**: Navigation — Deliveries tab + BlocProvider wiring
- [x] **Tests**: 78 delivery tests + 404 server tests (all pass, 297 total mobile)
- [ ] **T11**: Integration tests — QR offline flow, GPS capture, status transitions, sync
- [ ] **T12**: Photo upload endpoint — complete POST /deliveries/{id}/photos in DeliveryFunction

### Flutter Packages to Add

| Package | Purpose | Verified AOT/Size Safe |
|---------|---------|----------------------|
| `qr_flutter` | QR code display | Yes |
| `mobile_scanner` | QR code camera scanning | Yes |
| `geolocator` | GPS coordinates | Yes |
| `image_picker` | Camera/gallery photo selection | Yes |
| `image` | Image resize + WebP conversion | Yes |

### Acceptance Criteria

- [ ] Farmer can display QR code for pickup (offline)
- [ ] Trucker can scan QR code to confirm pickup (offline)
- [ ] GPS captured at pickup and delivery (with graceful fallback)
- [ ] Photo evidence captured, compressed to WebP <200KB
- [ ] Buyer can scan QR code to confirm delivery (offline)
- [ ] Dispute can be raised within 48h of delivery
- [ ] All delivery operations work offline (Isar + SyncQueue)
- [ ] Status transitions enforced (pending → picked_up → in_transit → delivered)
- [ ] Localized in English, Amharic, Afan Oromo
- [ ] Passes security review (HMAC verification, participant auth)

---

## Upcoming (After F-12)

| Order | Task | Blocked By |
|-------|------|------------|
| Next | F-11 Trip Matching (crop→trip algorithm) | F-12 completion |
| Then | F-13 Telebirr Escrow | F-12 delivery confirmation |
| Then | F-14 Fraud Detection | F-11 + F-12 + F-13 |
| Then | F-15 Conflict Resolution UI | F-08 sync engine |
| Then | F-18 S3 Access Logging | None (low priority) |

---

**Last Updated:** 2026-02-19
